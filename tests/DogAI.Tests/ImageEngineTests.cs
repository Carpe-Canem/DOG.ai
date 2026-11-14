using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Core.Net;
using DogAI.Engine;
using DogAI.Engine.Images;
using DogAI.Hub;

namespace DogAI.Tests;

/// <summary>
/// A pretend sd-server answering the "/sdcpp/v1" API from memory: a job moves through the states the real one reports
/// (queued, conditioning, sampling steps with previews, decoding, completed), one state per poll.
/// </summary>
internal sealed class FakeSdServer : HttpMessageHandler
{
    public static readonly byte[] Picture = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
    public static readonly byte[] Second = [0x89, (byte)'P', (byte)'N', (byte)'G', 4, 5, 6];
    public static readonly byte[] PreviewBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 9];

    public List<JsonObject> Started { get; } = [];
    public List<string> Requests { get; } = [];
    public int Steps { get; set; } = 3;

    /// <summary>Finish with "failed" and this message instead of pictures.</summary>
    public string? FailWith { get; set; }

    /// <summary>Answer img_gen with this status and body instead of starting a job.</summary>
    public (HttpStatusCode Status, string Body)? Refuse { get; set; }

    /// <summary>Keep sampling until cancelled (for cancel tests).</summary>
    public bool Endless { get; set; }

    /// <summary>Polls a job stays "generating" after a cancel request before it reports "cancelled".</summary>
    public int CancelLag { get; set; } = 2;

    public bool CancelRequested { get; private set; }
    private int _poll;
    private int _sinceCancel;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(request.Method + " " + path);
        if (request.Method == HttpMethod.Post && path == "/sdcpp/v1/img_gen")
        {
            Started.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if (Refuse is { } refuse) return Json(refuse.Status, refuse.Body);
            return Json(HttpStatusCode.Accepted, """{"id":"job-1","kind":"img_gen","status":"queued","created":1,"poll_url":"/sdcpp/v1/jobs/job-1"}""");
        }
        if (request.Method == HttpMethod.Post && path == "/sdcpp/v1/jobs/job-1/cancel")
        {
            CancelRequested = true;
            return Json(HttpStatusCode.OK, """{"id":"job-1","status":"generating","cancel_requested":true}""");
        }
        if (request.Method == HttpMethod.Get && path == "/sdcpp/v1/jobs/job-1")
            return Json(HttpStatusCode.OK, NextState());
        if (request.Method == HttpMethod.Get && path == "/sdcpp/v1/models/status")
            return Json(HttpStatusCode.OK, """{"error":null,"models":{},"recipe":null,"state":"ready"}""");
        if (request.Method == HttpMethod.Get && path == "/sdcpp/v1/capabilities")
            return Json(HttpStatusCode.OK, """
                {"samplers":["euler","euler_a"],"schedulers":[{"name":"karras"}],
                 "loras":[{"name":"Maid","path":"Maid_marian.safetensors","display_path":"Maid_marian.safetensors"}]}
                """);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string NextState()
    {
        var n = _poll++;
        if (CancelRequested && ++_sinceCancel > CancelLag)
            return """{"id":"job-1","status":"cancelled","progress":null,"preview":null,"result":null,"error":null}""";
        if (n == 0) return """{"id":"job-1","status":"queued","queue_position":0,"progress":null,"preview":null,"result":null,"error":null}""";
        if (n == 1)
            return """{"id":"job-1","status":"generating","progress":{"stage":"conditioning","detail":"positive_prompt","pass":1,"completed":null,"total":null,"unit":null},"preview":null}""";
        var step = n - 1;
        if (Endless || step <= Steps)
        {
            var shown = Endless ? Math.Min(step, 60) : step;
            // A preview every second step; the same preview is repeated on the polls between.
            var previewStep = shown - shown % 2;
            var preview = previewStep == 0 ? "null"
                : $$"""{"pass":1,"step":{{previewStep}},"total_steps":{{Steps}},"b64_json":"{{Convert.ToBase64String(PreviewBytes)}}"}""";
            return $$"""{"id":"job-1","status":"generating","progress":{"stage":"sampling","pass":1,"completed":{{shown}},"total":{{(Endless ? 60 : Steps)}},"unit":"steps"},"preview":{{preview}} }""";
        }
        if (step == Steps + 1)
            return """{"id":"job-1","status":"generating","progress":{"stage":"decoding","pass":1,"completed":null,"total":null,"unit":null},"preview":null}""";
        if (FailWith is { } message)
            return $$"""{"id":"job-1","status":"failed","error":{"code":"generation_failed","message":"{{message}}"} }""";
        return $$"""
            {"id":"job-1","status":"completed","progress":{"stage":"done","pass":1,"completed":null,"total":null,"unit":null},
             "result":{"output_format":"png","images":[
               {"index":1,"b64_json":"{{Convert.ToBase64String(Second)}}"},
               {"index":0,"b64_json":"{{Convert.ToBase64String(Picture)}}"}]},"error":null}
            """;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>A stand-in for the picture engine, for later steps' tests of code that asks for pictures.</summary>
internal sealed class FakeImageEngine : IImageEngine
{
    public ImageModel? Model { get; private set; }
    public bool IsBusy { get; private set; }

    /// <summary>Like the real engine: it holds its model in graphics memory from the first picture until it is let go.</summary>
    public bool IsRunning { get; private set; }
    public string? Error => null;
    public event Action? Changed;
    public List<ImageRequest> Requests { get; } = [];

    /// <summary>What happened, in order ("draw", "drawn", "release", "unload"); tests share one list with other fakes.</summary>
    public List<string> Events { get; init; } = [];

    /// <summary>When set, every picture stops after its first step until this completes (to do things mid-picture).</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Completes when a picture has reached <see cref="Hold"/>.</summary>
    public TaskCompletionSource Held { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task LoadAsync(ImageModel model, CancellationToken ct = default)
    {
        if (Model != model) IsRunning = false;
        Model = model;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken ct = default)
    {
        lock (Events) Events.Add("unload");
        Model = null;
        IsRunning = false;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken ct = default)
    {
        lock (Events) Events.Add("release");
        IsRunning = false;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<ImageResult> DrawAsync(ImageRequest request, IProgress<ImageProgress>? progress = null, CancellationToken ct = default)
    {
        if (Model is null) throw new ImageEngineException("No picture model is loaded.");
        if (request.Problems() is { Count: > 0 } problems) throw new ImageEngineException(string.Join(" ", problems));
        Requests.Add(request);
        lock (Events) Events.Add("draw");
        IsBusy = true;
        IsRunning = true;
        try
        {
            for (var step = 1; step <= request.Steps; step++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ImageProgress("sampling", step, request.Steps));
                if (step == 1 && Hold is { } hold)
                {
                    Held.TrySetResult();
                    await hold.Task.WaitAsync(ct);
                    Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                await Task.Yield();
            }
            lock (Events) Events.Add("drawn");
            var seed = request.Seed >= 0 ? request.Seed : 1234;
            return new ImageResult(Enumerable.Range(0, request.Count).Select(i => new[] { (byte)(seed + i) }).ToList(), request.Format, seed);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

internal sealed class Collect<T> : IProgress<T>
{
    public List<T> Items { get; } = [];
    public Action<T>? Then { get; set; }

    public void Report(T value)
    {
        lock (Items) Items.Add(value);
        Then?.Invoke(value);
    }
}

public class ImageRequestTests
{
    [Fact]
    public void A_plain_request_has_no_problems()
    {
        var request = new ImageRequest("1girl, silver hair", Width: 832, Height: 1216, Steps: 28, ClipSkip: 2,
            Loras: [new LoraUse("style/ink.safetensors", 0.8)], Hires: new HiresFix(1.5, 14, 0.5), Format: ImageFormat.Webp, Quality: 90);
        Assert.Empty(request.Problems());
    }

    [Theory]
    [InlineData("", 1024, 1024, 20, "prompt is empty")]
    [InlineData("x", 1004, 1024, 20, "multiple of 8")]
    [InlineData("x", 128, 1024, 20, "between 256 and 4096")]
    [InlineData("x", 1024, 1024, 0, "Steps must be")]
    [InlineData("x", 1024, 1024, 151, "Steps must be")]
    public void Bad_requests_are_explained(string prompt, int width, int height, int steps, string expected)
    {
        var problems = new ImageRequest(prompt, Width: width, Height: height, Steps: steps).Problems();
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a.safetensors", true)]
    [InlineData("chars/maid.safetensors", true)]
    [InlineData("chars\\maid.safetensors", true)]
    [InlineData("../outside.safetensors", false)]
    [InlineData("chars/../../x.safetensors", false)]
    [InlineData("C:\\loras\\a.safetensors", false)]
    [InlineData("/etc/a.safetensors", false)]
    [InlineData("\\\\server\\share\\a.safetensors", false)]
    [InlineData("", false)]
    [InlineData("a//b.safetensors", false)]
    public void LoRA_paths_must_stay_inside_their_folder(string path, bool ok)
    {
        Assert.Equal(ok, ImageRequest.IsSafeRelativePath(path));
        Assert.Equal(ok, new ImageRequest("x", Loras: [new LoraUse(path)]).Problems().Count == 0);
    }

    [Fact]
    public void Hires_fix_cannot_make_a_giant_picture()
    {
        var problems = new ImageRequest("x", Width: 2048, Height: 2048, Hires: new HiresFix(2.5, 10, 0.5)).Problems();
        Assert.Contains(problems, p => p.Contains("larger than", StringComparison.Ordinal));
    }
}

public class SdApiTests
{
    private static SdApi Api(FakeSdServer server) => new(new HttpClient(server), 5555) { PollInterval = TimeSpan.Zero };

    [Fact]
    public void The_request_body_has_every_setting_and_keeps_the_prompt_out_of_the_picture()
    {
        var request = new ImageRequest("1girl, forest", "lowres", 832, 1216, Seed: 7, Steps: 28, Sampler: "euler_a", Scheduler: "karras",
            Guidance: 5.5, ClipSkip: 2, Loras: [new LoraUse("chars\\maid.safetensors", 0.7)], Hires: new HiresFix(1.5, 14, 0.45),
            Format: ImageFormat.Webp, Quality: 90, Count: 2, Preview: true, PreviewEvery: 4);
        var body = SdApi.Body(request, 7);

        Assert.Equal("1girl, forest", (string?)body["prompt"]);
        Assert.Equal("lowres", (string?)body["negative_prompt"]);
        Assert.Equal(832, (int?)body["width"]);
        Assert.Equal(1216, (int?)body["height"]);
        Assert.Equal(7, (long?)body["seed"]);
        Assert.Equal(2, (int?)body["batch_count"]);
        Assert.Equal(2, (int?)body["clip_skip"]);
        Assert.Equal("webp", (string?)body["output_format"]);
        Assert.Equal(90, (int?)body["output_compression"]);
        Assert.False((bool?)body["embed_image_metadata"]);
        Assert.Equal("euler_a", (string?)body["sample_params"]!["sample_method"]);
        Assert.Equal(28, (int?)body["sample_params"]!["sample_steps"]);
        Assert.Equal("karras", (string?)body["sample_params"]!["scheduler"]);
        Assert.Equal(5.5, (double?)body["sample_params"]!["guidance"]!["txt_cfg"]);
        var lora = Assert.Single(body["lora"]!.AsArray())!;
        Assert.Equal("chars/maid.safetensors", (string?)lora["path"]);
        Assert.Equal(0.7, (double?)lora["multiplier"]);
        Assert.True((bool?)body["hires"]!["enabled"]);
        Assert.Equal(1.5, (double?)body["hires"]!["scale"]);
        Assert.Equal(14, (int?)body["hires"]!["steps"]);
        Assert.Equal(0.45, (double?)body["hires"]!["denoising_strength"]);
        Assert.Equal("Latent", (string?)body["hires"]!["upscaler"]);
        Assert.Equal("proj", (string?)body["preview"]);
        Assert.Equal(4, (int?)body["preview_interval"]);
    }

    [Fact]
    public void Optional_settings_are_left_to_the_engine()
    {
        var body = SdApi.Body(new ImageRequest("x"), 1);
        Assert.False(body.ContainsKey("clip_skip"));
        Assert.False(body.ContainsKey("hires"));
        Assert.False(body.ContainsKey("output_compression"));
        Assert.False(body["sample_params"]!.AsObject().ContainsKey("scheduler"));
        Assert.Equal("none", (string?)body["preview"]);
        Assert.Empty(body["lora"]!.AsArray());
        Assert.Equal("png", (string?)body["output_format"]);
    }

    [Fact]
    public async Task Draws_reports_each_step_once_and_returns_the_pictures_in_order()
    {
        var server = new FakeSdServer { Steps = 4 };
        var progress = new Collect<ImageProgress>();
        var result = await Api(server).DrawAsync(new ImageRequest("x", Count: 2, Preview: true), 99, progress, CancellationToken.None);

        Assert.Equal([FakeSdServer.Picture, FakeSdServer.Second], result.Images);
        Assert.Equal(99, result.Seed);
        Assert.Equal(ImageFormat.Png, result.Format);
        Assert.Equal(99, (long?)server.Started.Single()["seed"]);

        var stages = progress.Items.Select(p => p.Stage).Distinct().ToList();
        Assert.Equal(["conditioning", "sampling", "decoding", "done"], stages);
        var sampling = progress.Items.Where(p => p.Stage == "sampling").ToList();
        Assert.Equal([1, 2, 3, 4], sampling.Select(p => p.Step));
        Assert.All(sampling, p => Assert.Equal(4, p.TotalSteps));
        Assert.Null(progress.Items.First(p => p.Stage == "conditioning").Step);
        // Previews at steps 2 and 4, each passed on once although the engine repeats them.
        Assert.Equal(2, progress.Items.Count(p => p.Preview is not null));
        Assert.Equal(FakeSdServer.PreviewBytes, progress.Items.First(p => p.Preview is not null).Preview);
    }

    [Fact]
    public async Task A_failed_picture_says_what_the_engine_said()
    {
        var server = new FakeSdServer { FailWith = "out of memory" };
        var e = await Assert.ThrowsAsync<ImageEngineException>(() => Api(server).DrawAsync(new ImageRequest("x"), 1, null, CancellationToken.None));
        Assert.Contains("out of memory", e.Message);
    }

    [Fact]
    public async Task A_refused_request_is_said_plainly()
    {
        var server = new FakeSdServer { Refuse = (HttpStatusCode.ServiceUnavailable, """{"code":"model_not_loaded","error":"No model is loaded"}""") };
        var e = await Assert.ThrowsAsync<ImageEngineException>(() => Api(server).DrawAsync(new ImageRequest("x"), 1, null, CancellationToken.None));
        Assert.Equal("The picture engine has no model loaded.", e.Message);

        server.Refuse = (HttpStatusCode.BadRequest, """{"error":{"code":"invalid_argument","message":"unknown sampler"}}""");
        e = await Assert.ThrowsAsync<ImageEngineException>(() => Api(server).DrawAsync(new ImageRequest("x"), 1, null, CancellationToken.None));
        Assert.Contains("unknown sampler", e.Message);
        Assert.Contains("400", e.Message);
    }

    [Fact]
    public async Task Cancelling_stops_the_job_in_the_engine_and_waits_for_it()
    {
        var server = new FakeSdServer { Endless = true, CancelLag = 3 };
        using var cts = new CancellationTokenSource();
        var progress = new Collect<ImageProgress> { Then = p => { if (p.Step >= 5) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Api(server).DrawAsync(new ImageRequest("x"), 1, progress, cts.Token));

        Assert.True(server.CancelRequested);
        // It kept asking until the engine said the job was cancelled, so the next picture does not queue behind it.
        var afterCancel = server.Requests.SkipWhile(r => !r.EndsWith("/cancel", StringComparison.Ordinal)).Skip(1).ToList();
        Assert.True(afterCancel.Count >= 3, string.Join(", ", afterCancel));
    }

    [Fact]
    public async Task Reads_samplers_schedulers_and_loras()
    {
        var caps = await Api(new FakeSdServer()).CapabilitiesAsync();
        Assert.Equal(["euler", "euler_a"], caps.Samplers);
        Assert.Equal(["karras"], caps.Schedulers);
        Assert.Equal(("Maid", "Maid_marian.safetensors"), Assert.Single(caps.Loras));
    }

    [Fact]
    public void A_job_with_a_data_uri_and_an_old_style_error_still_reads()
    {
        var job = SdApi.ParseJob(JsonNode.Parse($$"""
            {"status":"failed","error":"boom","result":{"images":[{"index":0,"b64_json":"data:image/png;base64,{{Convert.ToBase64String([1, 2])}}"}]} }
            """)!.AsObject());
        Assert.Equal("boom", job.Error);
        Assert.Equal([1, 2], Assert.Single(job.Images));
        Assert.True(job.IsFinished);
    }
}

public class FakeImageEngineTests
{
    [Fact]
    public async Task The_fake_keeps_the_engine_rules()
    {
        IImageEngine engine = new FakeImageEngine();
        await Assert.ThrowsAsync<ImageEngineException>(() => engine.DrawAsync(new ImageRequest("x")));
        await engine.LoadAsync(new ImageModel("c.safetensors"));
        await Assert.ThrowsAsync<ImageEngineException>(() => engine.DrawAsync(new ImageRequest("")));
        var result = await engine.DrawAsync(new ImageRequest("x", Seed: 5, Count: 2, Steps: 3));
        Assert.Equal(5, result.Seed);
        Assert.Equal(2, result.Images.Count);
        await engine.UnloadAsync();
        Assert.Null(engine.Model);
    }
}

public class ImageServerTests
{
    [Fact]
    public void Starts_on_loopback_with_no_verbose_logging_and_a_blank_page()
    {
        var args = ImageServer.Arguments(new ImageModel(@"D:\m\c.safetensors", @"D:\m\loras", @"D:\m\vae.safetensors", ImagePrediction.V), 41234, @"D:\e\blank.html");
        string Value(string option) => args[args.ToList().IndexOf(option) + 1];

        Assert.Equal("127.0.0.1", Value("--listen-ip"));
        Assert.Equal("41234", Value("--listen-port"));
        Assert.Equal(@"D:\m\c.safetensors", Value("-m"));
        Assert.Equal(@"D:\m\loras", Value("--lora-model-dir"));
        Assert.Equal(@"D:\m\vae.safetensors", Value("--vae"));
        Assert.Equal("v", Value("--prediction"));
        Assert.Equal(@"D:\e\blank.html", Value("--serve-html-path"));
        Assert.Contains("--disable-image-metadata", args);
        Assert.DoesNotContain(args, a => a is "-v" or "--verbose" or "--log-level" or "--color");
        Assert.DoesNotContain(args, a => a.Contains("output", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_normal_checkpoint_gets_no_prediction_vae_or_lora_options()
    {
        var args = ImageServer.Arguments(new ImageModel(@"D:\m\c.safetensors"), 1, "b.html");
        Assert.DoesNotContain("--prediction", args);
        Assert.DoesNotContain("--vae", args);
        Assert.DoesNotContain("--lora-model-dir", args);
    }

    [Fact]
    public async Task Refuses_ckpt_files_and_drawing_without_a_model()
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            using var http = new LoopbackHttp();
            using var server = new ImageServer(http, new SdServerLocator(paths), paths);

            var ckpt = Path.Combine(root, "old.ckpt");
            await File.WriteAllTextAsync(ckpt, "x");
            var e = await Assert.ThrowsAsync<ImageEngineException>(() => server.LoadAsync(new ImageModel(ckpt)));
            Assert.Contains(".safetensors", e.Message);
            await Assert.ThrowsAsync<ImageEngineException>(() => server.LoadAsync(new ImageModel(Path.Combine(root, "missing.safetensors"))));

            e = await Assert.ThrowsAsync<ImageEngineException>(() => server.DrawAsync(new ImageRequest("x")));
            Assert.Contains("No picture model", e.Message);
            e = await Assert.ThrowsAsync<ImageEngineException>(() => server.DrawAsync(new ImageRequest("")));
            Assert.Contains("prompt is empty", e.Message);
            Assert.False(server.IsBusy);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Without_the_engine_installed_it_says_where_to_get_it()
    {
        if (Environment.GetEnvironmentVariable(SdServerLocator.OverrideVariable) is { Length: > 0 }) return;
        var root = Path.Combine(Path.GetTempPath(), "dogai-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var checkpoint = Path.Combine(root, "c.safetensors");
            await File.WriteAllTextAsync(checkpoint, "x");
            using var http = new LoopbackHttp();
            using var server = new ImageServer(http, new SdServerLocator(paths), paths);
            var changes = 0;
            server.Changed += () => changes++;

            var e = await Assert.ThrowsAsync<ImageEngineException>(() => server.LoadAsync(new ImageModel(checkpoint)));
            Assert.Equal(SdServerLocator.MissingMessage, e.Message);
            Assert.Equal(SdServerLocator.MissingMessage, server.Error);
            Assert.False(server.IsRunning);
            Assert.True(changes > 0);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_locator_looks_only_in_the_engine_folder_and_the_override()
    {
        if (Environment.GetEnvironmentVariable(SdServerLocator.OverrideVariable) is { Length: > 0 }) return;
        var root = Path.Combine(Path.GetTempPath(), "dogai-img-" + Guid.NewGuid().ToString("N"));
        var onPath = Path.Combine(root, "onpath");
        Directory.CreateDirectory(onPath);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(onPath, SdServerLocator.FileName), "x");
            Environment.SetEnvironmentVariable("PATH", onPath + Path.PathSeparator + oldPath);
            var paths = new AppPaths(root);
            var locator = new SdServerLocator(paths);
            Assert.Null(locator.Find());

            Directory.CreateDirectory(paths.ImageEngineDirectory);
            var own = Path.Combine(paths.ImageEngineDirectory, SdServerLocator.FileName);
            File.WriteAllText(own, "x");
            Assert.Equal(own, locator.Find());
            Assert.StartsWith(paths.EngineDirectory, own, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(root, true);
        }
    }
}

public class ImageEngineUpdaterTests
{
    private const string Tag = "master-1094-3bad892";
    private const string ProgramZip = "sd-master-3bad892-bin-win-cuda12-x64.zip";
    private const string RuntimeZip = "cudart-sd-bin-win-cu12-x64.zip";

    private static EngineUpdater.ApiAsset Asset(string name, string? digest = "sha256:aa") => new(name, 100, digest, "https://github.com/leejet/stable-diffusion.cpp/releases/download/" + Tag + "/" + name);

    [Fact]
    public void Picks_the_cuda12_build_and_its_runtime()
    {
        EngineUpdater.ApiAsset[] assets =
        [
            Asset("sd-master-3bad892-bin-win-cpu-x64.zip"), Asset(ProgramZip), Asset("sd-master-3bad892-bin-win-vulkan-x64.zip"),
            Asset("sd-master-3bad892-bin-win-rocm-10.1.0-x64.zip"), Asset(RuntimeZip), Asset("sd-master-3bad892-bin-Darwin-macOS-26.7.2-arm64.zip"),
        ];
        var pick = ImageEngineUpdater.Pick(assets)!;
        Assert.Equal(ProgramZip, pick.Program.Name);
        Assert.Equal(RuntimeZip, pick.Runtime?.Name);
        Assert.Equal("aa", pick.Program.Sha256);
        Assert.Equal(200, pick.TotalBytes);
    }

    [Fact]
    public void A_release_missing_either_file_is_not_offered()
    {
        Assert.Null(ImageEngineUpdater.Pick([Asset(ProgramZip)]));
        Assert.Null(ImageEngineUpdater.Pick([Asset(RuntimeZip), Asset("sd-master-3bad892-bin-win-vulkan-x64.zip")]));
    }

    [Fact]
    public async Task Needs_an_nvidia_driver_with_cuda_12()
    {
        Assert.NotNull(ImageEngineUpdater.Unsuitable(null));
        Assert.Contains("CUDA 11.8", ImageEngineUpdater.Unsuitable(new Version(11, 8)));
        Assert.Null(ImageEngineUpdater.Unsuitable(new Version(12, 4)));
        Assert.Null(ImageEngineUpdater.Unsuitable(new Version(13, 1)));

        var server = new FileServer();
        var updater = new ImageEngineUpdater(new HttpClient(server), new AppPaths(Path.GetTempPath()));
        await Assert.ThrowsAsync<HubException>(() => updater.LatestAsync(null));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void The_release_addresses_are_on_the_allow_list_already()
    {
        foreach (var url in new[]
        {
            "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest",
            "https://github.com/leejet/stable-diffusion.cpp/releases/download/master-1094-3bad892/sd-master-3bad892-bin-win-cuda12-x64.zip",
            // Where GitHub redirects release downloads.
            "https://release-assets.githubusercontent.com/github-production-release-asset/1/abc",
            "https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/abc",
        })
            Assert.True(OriginPolicy.IsAllowedHubUri(new Uri(url)), url);
    }

    private sealed record Fixture(string Root, AppPaths Paths, FileServer Server, byte[] Program, byte[] Runtime) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(archive.CreateEntry(name).Open());
                w.Write(text);
            }
        }
        return bytes.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static Fixture Release(string? programDigest = null, bool omitDigest = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-sd-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        // As in the real archives: the program and its DLLs in the CUDA zip, NVIDIA's DLLs in the runtime zip.
        var program = Zip(("sd-server.exe", "pretend server"), ("ggml.dll", "sd ggml"), ("stable-diffusion.dll", "sd"));
        var runtime = Zip(("cudart64_12.dll", "runtime"), ("cublas64_12.dll", "blas"));
        var server = new FileServer();
        var digest = omitDigest ? "null" : $"\"sha256:{programDigest ?? Sha(program)}\"";
        server.Files["https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest"] = Encoding.UTF8.GetBytes($$"""
            {"tag_name":"{{Tag}}","published_at":"2026-11-13T20:08:16Z","assets":[
             {"name":"{{ProgramZip}}","size":{{program.Length}},"digest":{{digest}},"browser_download_url":"https://github.com/d/{{ProgramZip}}"},
             {"name":"{{RuntimeZip}}","size":{{runtime.Length}},"digest":"sha256:{{Sha(runtime)}}","browser_download_url":"https://github.com/d/{{RuntimeZip}}"}]}
            """);
        server.Files[$"https://github.com/d/{ProgramZip}"] = program;
        server.Files[$"https://github.com/d/{RuntimeZip}"] = runtime;
        return new Fixture(root, paths, server, program, runtime);
    }

    [Fact]
    public async Task Installs_both_files_into_its_own_folder_beside_llama_cpp()
    {
        using var f = Release();
        // llama.cpp is already installed; its files must not be touched or mixed with the picture engine's.
        Directory.CreateDirectory(f.Paths.EngineDirectory);
        await File.WriteAllTextAsync(Path.Combine(f.Paths.EngineDirectory, "llama-server.exe"), "llama");
        await File.WriteAllTextAsync(Path.Combine(f.Paths.EngineDirectory, "ggml.dll"), "llama ggml");

        var updater = new ImageEngineUpdater(new HttpClient(f.Server), f.Paths);
        Assert.False(updater.IsInstalled);
        var release = (await updater.LatestAsync(new Version(13, 0)))!;
        Assert.Equal(Tag, release.Tag);
        long last = 0;
        await updater.InstallAsync(release, n => last = n);

        Assert.True(updater.IsInstalled);
        Assert.Equal(Tag, updater.InstalledTag);
        Assert.Equal(release.TotalBytes, last);
        var dir = f.Paths.ImageEngineDirectory;
        Assert.Equal("sd ggml", await File.ReadAllTextAsync(Path.Combine(dir, "ggml.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "cudart64_12.dll")));
        Assert.Equal("llama ggml", await File.ReadAllTextAsync(Path.Combine(f.Paths.EngineDirectory, "ggml.dll")));
        Assert.True(File.Exists(Path.Combine(f.Paths.EngineDirectory, "llama-server.exe")));
        // Nothing left over from the download.
        Assert.False(Directory.Exists(Path.Combine(f.Root, "image-engine-download")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, "image-engine-new")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, "image-engine-old")));
    }

    [Fact]
    public async Task A_damaged_download_leaves_the_old_engine()
    {
        using var f = Release(programDigest: new string('b', 64));
        Directory.CreateDirectory(f.Paths.ImageEngineDirectory);
        await File.WriteAllTextAsync(Path.Combine(f.Paths.ImageEngineDirectory, "sd-server.exe"), "old");
        var updater = new ImageEngineUpdater(new HttpClient(f.Server), f.Paths);

        var e = await Assert.ThrowsAsync<HubException>(async () => await updater.InstallAsync((await updater.LatestAsync(new Version(12, 4)))!, _ => { }));
        Assert.Contains("checksum", e.Message);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(f.Paths.ImageEngineDirectory, "sd-server.exe")));
    }

    [Fact]
    public async Task A_file_without_a_published_checksum_is_refused()
    {
        using var f = Release(omitDigest: true);
        var updater = new ImageEngineUpdater(new HttpClient(f.Server), f.Paths);
        var e = await Assert.ThrowsAsync<HubException>(async () => await updater.InstallAsync((await updater.LatestAsync(new Version(12, 4)))!, _ => { }));
        Assert.Contains("no published checksum", e.Message);
        Assert.DoesNotContain(f.Server.Requests, r => r.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal));
        Assert.False(updater.IsInstalled);
    }

    [Fact]
    public async Task Updating_llama_cpp_keeps_the_picture_engine()
    {
        using var f = Release();
        var pictures = new ImageEngineUpdater(new HttpClient(f.Server), f.Paths);
        await pictures.InstallAsync((await pictures.LatestAsync(new Version(12, 4)))!, _ => { });

        var llamaZip = Zip(("llama-server.exe", "new llama"));
        f.Server.Files["https://api.github.com/repos/ggml-org/llama.cpp/releases/latest"] = Encoding.UTF8.GetBytes($$"""
            {"tag_name":"b12110","published_at":"2026-11-01T00:00:00Z","assets":[
             {"name":"llama-b12110-bin-win-vulkan-x64.zip","size":{{llamaZip.Length}},"digest":"sha256:{{Sha(llamaZip)}}","browser_download_url":"https://github.com/r/v.zip"}]}
            """);
        f.Server.Files["https://github.com/r/v.zip"] = llamaZip;
        var llama = new EngineUpdater(new HttpClient(f.Server), f.Paths);
        await File.WriteAllTextAsync(Path.Combine(f.Paths.EngineDirectory, "old.txt"), "old llama file");
        await llama.InstallAsync((await llama.LatestAsync(null))!, _ => { });

        Assert.Equal("b12110", llama.InstalledTag);
        Assert.False(File.Exists(Path.Combine(f.Paths.EngineDirectory, "old.txt")));
        Assert.True(pictures.IsInstalled);
        Assert.Equal(Tag, pictures.InstalledTag);
        Assert.Equal("sd ggml", await File.ReadAllTextAsync(Path.Combine(f.Paths.ImageEngineDirectory, "ggml.dll")));

        // And the other way round: updating the picture engine leaves llama.cpp alone.
        await pictures.InstallAsync((await pictures.LatestAsync(new Version(12, 4)))!, _ => { });
        Assert.Equal("new llama", await File.ReadAllTextAsync(Path.Combine(f.Paths.EngineDirectory, "llama-server.exe")));
        Assert.Equal("b12110", llama.InstalledTag);
        Assert.True(pictures.IsInstalled);
    }
}
