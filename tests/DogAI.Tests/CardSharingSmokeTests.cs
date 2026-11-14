using System.Diagnostics;
using System.Text;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Engine;
using DogAI.Engine.Images;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// A real chat model and a real picture sharing the real graphics card, with its memory measured along the way.
/// Skipped unless DOGAI_TEST_MODEL (a GGUF chat model), DOGAI_SD_SERVER (sd-server.exe) and DOGAI_TEST_IMAGE_MODEL
/// (an SDXL checkpoint) are all set. Whether the chat model has to make room depends on its size: the test works out
/// what should happen from the measured card and checks that it did. Takes a minute or two; the card should be free.
/// </summary>
public class CardSharingSmokeTests(ITestOutputHelper output)
{
    private sealed class NoRecords : IModelRecordStore
    {
        public Task<IReadOnlyList<ModelRecord>> AllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelRecord>>([]);
        public Task<ModelRecord?> FindAsync(string path, CancellationToken ct = default) => Task.FromResult<ModelRecord?>(null);
        public Task SaveAsync(ModelRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0") + " GB";

    /// <summary>
    /// A model the owner asks for while a page is still loading its default does not wait for that load. Every load takes
    /// a turn on the graphics card first, so the wanted load would otherwise wait behind the page's until it had finished;
    /// instead the page's load is stopped and the wanted model loads at once. Needs only DOGAI_TEST_MODEL (no picture is
    /// drawn); the two loads are the same file with different context sizes, so they can be told apart.
    /// </summary>
    [Fact]
    public async Task A_wanted_load_does_not_wait_for_a_pages_own_load()
    {
        var chatModel = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        if (string.IsNullOrEmpty(chatModel)) return;

        var root = Path.Combine(Path.GetTempPath(), "dogai-cardsmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            using var http = new LoopbackHttp();
            using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
            using var server = new ImageServer(http, new SdServerLocator(paths), paths);
            var probe = new HardwareProbe();
            // The coordinator is what makes loads take turns.
            _ = new CardCoordinator(server, engine, new CardMeter(probe, engine, server));
            var launcher = new ModelLauncher(engine, probe, new NoRecords());
            var clock = Stopwatch.StartNew();
            var states = new List<EngineState>();
            engine.Changed += () =>
            {
                lock (states)
                {
                    if (states.Count > 0 && states[^1] == engine.State) return;
                    states.Add(engine.State);
                }
                output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] chat engine: {engine.State}");
            };

            // A page opens and loads its default: nobody asked for it.
            var pageLoad = launcher.LoadAsync(chatModel, 4096, automatic: true);
            for (var i = 0; i < 400 && engine.State != EngineState.Loading; i++) await Task.Delay(25);
            Assert.Equal(EngineState.Loading, engine.State);
            await Task.Delay(1500); // well into the load

            // The owner asks for a model.
            var asked = Stopwatch.StartNew();
            await launcher.LoadAsync(chatModel, 2048);
            output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] the wanted model was ready {asked.Elapsed.TotalSeconds:0.0} s after it was asked for");
            var held = engine.ProcessId is int pid ? await probe.VramOfProcessAsync(pid) : 0;
            output.WriteLine($"         layers asked for on the graphics card: {engine.GpuLayers} (-1 = all); it holds {Gb(held)} there");
            Assert.Equal(EngineState.Ready, engine.State);
            Assert.Equal(2048, engine.ContextLength);
            await pageLoad.WaitAsync(TimeSpan.FromSeconds(10));

            // The page's load never finished: the engine went Loading, Idle, Loading, Ready, and was ready only once.
            lock (states)
            {
                Assert.Equal(1, states.Count(s => s == EngineState.Ready));
                Assert.Contains(EngineState.Idle, states);
            }

            // A page's own load that comes late does not replace the model that is in.
            await launcher.LoadAsync(chatModel, 4096, automatic: true);
            Assert.Equal(EngineState.Ready, engine.State);
            Assert.Equal(2048, engine.ContextLength);

            await engine.UnloadAsync();
            Assert.Equal(EngineState.Idle, engine.State);

            // The same when the owner asks in the first moment, before the page's load has reached the engine (the only
            // moment the Load buttons are still on): whichever of the two gets its turn first, the wanted model is the
            // one that ends up loaded, and only one load runs to the end.
            lock (states) states.Clear();
            var early = launcher.LoadAsync(chatModel, 4096, automatic: true);
            asked.Restart();
            await launcher.LoadAsync(chatModel, 2048);
            output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] asked in the first moment: ready {asked.Elapsed.TotalSeconds:0.0} s later");
            await early.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EngineState.Ready, engine.State);
            Assert.Equal(2048, engine.ContextLength);
            lock (states) Assert.Equal(1, states.Count(s => s == EngineState.Ready));

            await engine.UnloadAsync();
            Assert.Equal(EngineState.Idle, engine.State);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task A_real_chat_model_and_real_pictures_share_the_card()
    {
        var chatModel = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        var exe = Environment.GetEnvironmentVariable(SdServerLocator.OverrideVariable);
        var checkpoint = Environment.GetEnvironmentVariable(ImageSmokeTests.ModelVariable);
        if (string.IsNullOrEmpty(chatModel) || string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(checkpoint)) return;

        var root = Path.Combine(Path.GetTempPath(), "dogai-cardsmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            using var http = new LoopbackHttp();
            using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
            using var server = new ImageServer(http, new SdServerLocator(paths), paths);
            var probe = new HardwareProbe();
            var meter = new CardMeter(probe, engine, server);
            var card = new CardCoordinator(server, engine, meter);
            card.Noted += note => output.WriteLine("  note: " + note);
            engine.Changed += () => output.WriteLine($"  chat engine: {engine.State}");
            var launcher = new ModelLauncher(engine, probe, new NoRecords());
            var clock = Stopwatch.StartNew();

            async Task<CardMemory> MeasureAsync(string when)
            {
                var m = await meter.ReadAsync() ?? throw new InvalidOperationException("The graphics card cannot be measured.");
                output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] {when}: {Gb(m.TotalBytes - m.FreeBytes)} used, {Gb(m.FreeBytes)} free (chat model {Gb(m.ChatBytes)}, picture model {Gb(m.PictureBytes)})");
                return m;
            }

            async Task<string> AskAsync()
            {
                var reply = new StringBuilder();
                // Without thinking: a model that thinks first can spend all 40 tokens on it and answer nothing.
                await foreach (var delta in engine.StreamChatAsync([new("user", "Reply with one short sentence about dogs.")], SamplerPresets.Get("precise") with { MaxTokens = 40, Thinking = false }))
                    reply.Append(delta.Content);
                return reply.ToString().Trim();
            }

            // Draws while watching the card four times a second, and says how full it got.
            async Task<ImageResult> DrawAsync(string what, ImageRequest request)
            {
                long peak = 0;
                using var stop = new CancellationTokenSource();
                var watch = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (await probe.MainGpuAsync() is { } gpu) peak = Math.Max(peak, gpu.TotalBytes - gpu.FreeBytes);
                        try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { }
                    }
                });
                var start = clock.Elapsed;
                var result = await card.DrawAsync(request);
                stop.Cancel();
                await watch;
                output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] {what}: drawn in {(clock.Elapsed - start).TotalSeconds:0.0} s, decision {card.LastDecision!.Choice}, card peaked at {Gb(peak)} used");
                Assert.True(result.Images[0].Length > 10_000);
                return result;
            }

            var before = await MeasureAsync("before anything");

            await launcher.LoadAsync(chatModel, 8192);
            Assert.True(engine.IsLoaded, engine.Error);
            var context = engine.ContextLength;
            Assert.False(string.IsNullOrWhiteSpace(await AskAsync()));
            var loaded = await MeasureAsync("chat model loaded and answered");
            Assert.True(loaded.ChatBytes > 0, "the chat model's graphics memory could not be measured");

            await card.LoadAsync(new ImageModel(checkpoint));
            var standard = new ImageRequest("1girl, silver hair, green eyes, forest", "lowres, bad anatomy", 832, 1216, Seed: 1, Steps: 28, Guidance: 5.5, ClipSkip: 2);
            var need = PictureMemory.Need(new FileInfo(checkpoint).Length, 0, standard);
            var swap = !PictureMemory.FitsBeside(await MeasureAsync("picture engine started, nothing drawn yet"), need);
            output.WriteLine($"a Standard picture needs about {Gb(need)}: the chat model {(swap ? "must make room" : "can stay")}");

            // 1. The first picture.
            await DrawAsync("first picture", standard);
            Assert.Equal(swap ? CardChoice.SetChatAside : CardChoice.KeptBoth, card.LastDecision!.Choice);
            var afterFirst = await MeasureAsync("after the first picture");
            if (swap)
            {
                Assert.Equal(EngineState.Resting, engine.State);
                Assert.Null(engine.ProcessId);
                Assert.Equal(0, afterFirst.ChatBytes);
                // Still "ready" to the chat pages, with its name and context size, though it holds no memory.
                Assert.True(engine.IsReady);
                Assert.False(engine.IsLoaded);
                Assert.Equal(context, engine.ContextLength);
                Assert.False(string.IsNullOrEmpty(engine.ModelName));
            }
            else
            {
                Assert.True(engine.IsLoaded);
            }

            // 2. "Try again": no second swap, and the chat model is not loaded again in between.
            await DrawAsync("second picture (try again)", standard with { Seed = 2 });
            if (swap)
            {
                Assert.Equal(CardChoice.NothingLoaded, card.LastDecision!.Choice);
                Assert.Equal(EngineState.Resting, engine.State);
            }
            var beforeMessage = await MeasureAsync("after the second picture, picture model idle");
            Assert.True(beforeMessage.PictureBytes > 0, "the picture model's graphics memory could not be measured");

            // 3. The next message brings the chat model back by itself.
            var wake = Stopwatch.StartNew();
            var answer = await AskAsync();
            output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] next message answered after {wake.Elapsed.TotalSeconds:0.0} s: \"{answer}\"");
            Assert.False(string.IsNullOrWhiteSpace(answer));
            Assert.Equal(EngineState.Ready, engine.State);
            Assert.Equal(context, engine.ContextLength);
            var back = await MeasureAsync("chat model answered the next message");
            if (swap)
            {
                var fitsBack = beforeMessage.FreeBytes >= loaded.ChatBytes + PictureMemory.Margin;
                Assert.Equal(fitsBack, server.IsRunning);
                // It came back whole: about as much on the card as before, not squeezed beside the picture model.
                Assert.InRange(back.ChatBytes, loaded.ChatBytes * 9 / 10, loaded.ChatBytes * 11 / 10);
            }
            Assert.NotNull(card.Model);

            // 4. A picture after that works again (and swaps again when it has to).
            await DrawAsync("third picture, after chatting", standard with { Seed = 3 });
            if (swap) Assert.Equal(EngineState.Resting, engine.State);

            // 5. Unloaded by the owner while set aside: it stays unloaded, for the next message and for default loading.
            await launcher.UnloadByUserAsync();
            Assert.Equal(EngineState.Idle, engine.State);
            await Assert.ThrowsAsync<EngineException>(() => engine.CountTokensAsync("x"));
            var settings = new AppSettings { DefaultModels = new Dictionary<string, string> { ["general"] = chatModel } };
            Assert.Null(await launcher.LoadDefaultIfIdleAsync(settings, "general"));
            await DrawAsync("fourth picture, chat model unloaded by the owner", standard with { Seed = 4 });
            Assert.Equal(EngineState.Idle, engine.State);

            await card.UnloadAsync();
            await Task.Delay(1000);
            var end = await MeasureAsync("everything unloaded");
            Assert.InRange(end.FreeBytes, before.FreeBytes - (1L << 30), long.MaxValue);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
