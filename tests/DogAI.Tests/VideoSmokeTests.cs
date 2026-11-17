using System.Diagnostics;
using System.Text;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Core.Video;
using DogAI.Engine;
using DogAI.Engine.Images;
using DogAI.Engine.Video;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// Makes real clips with the real picture engine and a real video model. Skipped unless DOGAI_SD_SERVER points at
/// sd-server.exe and DOGAI_TEST_VIDEO_DIR at a video models folder holding Wan 2.2 TI2V-5B's four files in
/// <c>diffusion_models</c>, <c>text_encoders</c> and <c>vae</c>. The clips are short (33 frames, 8 steps) so the
/// whole class takes about three minutes; the card should be free (it needs about 15 GB). Nothing is written to
/// disk: every clip stays in memory and is thrown away.
/// </summary>
public class VideoSmokeTests(ITestOutputHelper output)
{
    public const string FolderVariable = "DOGAI_TEST_VIDEO_DIR";
    private const string Marker = "zqxvideomarker";

    private static (string Exe, VideoModel Model)? Setup()
    {
        var exe = Environment.GetEnvironmentVariable(SdServerLocator.OverrideVariable);
        var folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(folder)) return null;
        return (exe, VideoFolders.ModelFor(folder, VideoFiles.Wan22));
    }

    /// <summary>A short clip: a quarter of the length and under half the steps of a real one, at the trial's smallest size.</summary>
    private static VideoRequest Short(string sentence, int width = 832, int height = 480) =>
        new(sentence, width, height, Seed: 4321, Frames: 33, Steps: 8);

    [Fact]
    public async Task Makes_clips_from_a_sentence_and_from_a_first_frame_and_leaves_nothing_behind()
    {
        if (Setup() is not { } setup) return;
        var root = Path.Combine(Path.GetTempPath(), "dogai-videosmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engineFolder = Path.GetDirectoryName(setup.Exe)!;
        var engineFilesBefore = Listing(engineFolder);
        var lines = new List<string>();
        var logged = new List<string>();
        try
        {
            var paths = new AppPaths(root);
            using var http = new LoopbackHttp();
            using var server = new VideoServer(http, new SdServerLocator(paths), paths);
            server.Output += line => { lock (lines) lines.Add(line); };
            server.Logged += line => { lock (logged) logged.Add(line); };
            var probe = new HardwareProbe();
            var freeBefore = (await probe.MainGpuAsync())?.FreeBytes ?? 0;

            // 1. From a sentence, with the fast decoder and its full-size previews.
            var progress = new Collect<VideoProgress>();
            var clock = Stopwatch.StartNew();
            var sentence = Short($"A mountain lake at dawn, mist drifting over the water, {Marker}") with { Preview = true, PreviewEvery = 2 };
            var first = await server.MakeAsync(setup.Model, sentence, progress);
            output.WriteLine($"sentence clip: {clock.Elapsed.TotalSeconds:0.0} s, {first.Webm.Length:N0} bytes; estimate {VideoEstimate.For(sentence).Total:0} s cold, {VideoEstimate.For(sentence, warm: true).Total:0} s warm");
            output.WriteLine("stages: " + string.Join(", ", progress.Items.Select(p => p.Stage).Distinct()));
            output.WriteLine(Tree(first.Webm));
            var info = WebmInfo.Read(first.Webm, out var problem);
            Assert.True(info is not null, problem);
            output.WriteLine($"webm: {info}");
            Assert.Equal((832, 480, 33), (first.Width, first.Height, first.Frames));
            Assert.Equal(4321, first.Seed);
            Assert.False(server.IsRunning);
            Assert.False(server.IsBusy);

            var sampling = progress.Items.Where(p => p.Stage == VideoStages.Drawing).ToList();
            Assert.NotEmpty(sampling);
            Assert.All(sampling, p => Assert.Equal(8, p.TotalSteps));
            Assert.Contains(progress.Items, p => p.Stage == VideoStages.Starting);
            var previews = progress.Items.Where(p => p.Preview is not null).ToList();
            Assert.NotEmpty(previews);
            Assert.All(previews, p => Assert.True(IsPng(p.Preview!)));
            output.WriteLine($"previews: {previews.Count}, the first {previews[0].Preview!.Length:N0} bytes");

            // The sentence is nowhere in the clip (the engine was started with its metadata switch).
            Assert.False(Holds(first.Webm, Marker));
            Assert.False(Holds(first.Webm, "mountain"));
            Assert.False(Holds(first.Webm, "wan2.2"));

            // 2. The same seed and settings give the same clip.
            clock.Restart();
            var again = await server.MakeAsync(setup.Model, sentence with { Preview = false });
            // The file itself differs in a few bytes each time (the muxer gives the track a random number), the frames do not.
            var different = first.Webm.Length == again.Webm.Length ? first.Webm.Zip(again.Webm).Count(pair => pair.First != pair.Second) : -1;
            output.WriteLine($"again (program started anew, files in Windows' cache): {clock.Elapsed.TotalSeconds:0.0} s; {different} bytes differ (-1: another length)");
            Assert.InRange(different, 0, 16);
            Save(first.Webm, "sentence.webm");

            // 3. From a first frame: a real JPEG of the clip's own shape (1024 × 640 into 768 × 480).
            var frame = TestJpegs.Real("sign-1024x640.jpg");
            var framed = Short($"The sign sways gently in the wind, {Marker}", 768, 480) with { StartFrame = frame };
            Assert.Empty(framed.Problems());
            var withFrame = new Collect<VideoProgress>();
            clock.Restart();
            var second = await server.MakeAsync(setup.Model, framed, withFrame);
            output.WriteLine($"first-frame clip: {clock.Elapsed.TotalSeconds:0.0} s, {second.Webm.Length:N0} bytes; stages {string.Join(", ", withFrame.Items.Select(p => p.Stage).Distinct())}");
            Assert.Equal((768, 480, 33), (second.Width, second.Height, second.Frames));
            // Reading the first frame takes half a second, so its stage is not always seen. That the frame was used
            // shows in the clip: the same request without it gives another one.
            var unframed = await server.MakeAsync(setup.Model, framed with { StartFrame = null });
            Assert.False(second.Webm.AsSpan().SequenceEqual(unframed.Webm));
            output.WriteLine($"the same without the first frame: {unframed.Webm.Length:N0} bytes");
            Save(second.Webm, "first-frame.webm");
            Save(unframed.Webm, "first-frame-without.webm");
            Assert.False(Holds(second.Webm, Marker));
            // The first frame was sent as bytes; no piece of the JPEG file is in the clip.
            Assert.True(second.Webm.AsSpan().IndexOf(frame.AsSpan(200, 64)) < 0);

            // 4. The model's own decoder: the program is started with the other switch.
            clock.Restart();
            var own = await server.MakeAsync(setup.Model, sentence with { Preview = false, Decoder = VideoDecoder.Own });
            output.WriteLine($"own decoder: {clock.Elapsed.TotalSeconds:0.0} s, {own.Webm.Length:N0} bytes");
            Assert.Equal((832, 480, 33), (own.Width, own.Height, own.Frames));
            Assert.False(first.Webm.AsSpan().SequenceEqual(own.Webm));

            // 5. Cancel while it is drawn: back within seconds, and the program is gone.
            using (var cts = new CancellationTokenSource())
            {
                var watch = new Collect<VideoProgress> { Then = p => { if (p.Stage == VideoStages.Drawing && p.Step >= 2) cts.Cancel(); } };
                var cancelled = Stopwatch.StartNew();
                cts.Token.Register(() => cancelled.Restart());
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    server.MakeAsync(setup.Model, sentence with { Steps = 30, Frames = 121, Preview = false, Decoder = VideoDecoder.Own }, watch, cts.Token));
                output.WriteLine($"cancel came back {cancelled.Elapsed.TotalSeconds:0.0} s after it was asked for");
                Assert.True(cancelled.Elapsed < TimeSpan.FromSeconds(12), $"cancel took {cancelled.Elapsed.TotalSeconds:0.0} s");
                Assert.False(server.IsRunning);
            }

            // Nothing is left on the card, and no engine is left running.
            await Task.Delay(1500);
            var freeAfter = (await probe.MainGpuAsync())?.FreeBytes ?? 0;
            output.WriteLine($"free graphics memory: {freeBefore / 1073741824.0:0.0} GB before, {freeAfter / 1073741824.0:0.0} GB after");
            Assert.True(freeAfter > freeBefore - (1L << 30), "the video program left something on the card");

            // What it printed never holds the sentence, and what would be logged shares no word with it.
            lock (lines)
            {
                output.WriteLine($"the engine printed {lines.Count} lines; {logged.Count} would be logged");
                Assert.DoesNotContain(lines, l => l.Contains(Marker, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(lines, l => l.Contains("mist drifting", StringComparison.OrdinalIgnoreCase));
            }
            lock (logged)
            {
                foreach (var word in new[] { Marker, "mountain", "drifting", "sways", "gently" })
                    Assert.DoesNotContain(logged, l => l.Contains(word, StringComparison.OrdinalIgnoreCase));
                foreach (var line in logged.Take(12)) output.WriteLine("  log: " + line);
            }

            // The engine wrote nothing beside itself, and DOG.ai nothing but its blank page.
            Assert.Equal(engineFilesBefore, Listing(engineFolder));
            var mine = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
            Assert.Equal(["dogai-blank.html"], mine);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class NoRecords : IModelRecordStore
    {
        public Task<IReadOnlyList<ModelRecord>> AllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelRecord>>([]);
        public Task<ModelRecord?> FindAsync(string path, CancellationToken ct = default) => Task.FromResult<ModelRecord?>(null);
        public Task SaveAsync(ModelRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0") + " GB";

    /// <summary>
    /// A real chat model and a real clip of the real length (five seconds at Quick's size) on the real card: the chat
    /// model is set aside for the clip, the clip takes about what the estimate said, nothing is left on the card, and
    /// the chat model comes back and answers when it is next asked. Needs DOGAI_TEST_MODEL (a GGUF chat model) as well.
    /// Takes about four minutes.
    /// </summary>
    [Fact]
    public async Task A_real_chat_model_and_a_real_clip_share_the_card()
    {
        var chatModel = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        if (Setup() is not { } setup || string.IsNullOrEmpty(chatModel)) return;

        var root = Path.Combine(Path.GetTempPath(), "dogai-videocard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            using var http = new LoopbackHttp();
            using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
            using var pictures = new ImageServer(http, new SdServerLocator(paths), paths);
            using var video = new VideoServer(http, new SdServerLocator(paths), paths);
            var probe = new HardwareProbe();
            var meter = new CardMeter(probe, engine, pictures);
            var card = new CardCoordinator(pictures, engine, meter, video: video);
            IVideoEngine videos = card;
            card.Noted += note => output.WriteLine("  note: " + note);
            var launcher = new ModelLauncher(engine, probe, new NoRecords());
            var clock = Stopwatch.StartNew();

            async Task<CardMemory> MeasureAsync(string when)
            {
                var m = await meter.ReadAsync() ?? throw new InvalidOperationException("The graphics card cannot be measured.");
                output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] {when}: {Gb(m.TotalBytes - m.FreeBytes)} used, {Gb(m.FreeBytes)} free (chat model {Gb(m.ChatBytes)})");
                return m;
            }

            async Task<string> AskAsync()
            {
                var reply = new StringBuilder();
                await foreach (var delta in engine.StreamChatAsync([new("user", "Reply with one short sentence about dogs.")], SamplerPresets.Get("precise") with { MaxTokens = 40, Thinking = false }))
                    reply.Append(delta.Content);
                return reply.ToString().Trim();
            }

            var before = await MeasureAsync("before anything");
            await launcher.LoadAsync(chatModel, 8192);
            Assert.True(engine.IsLoaded, engine.Error);
            Assert.False(string.IsNullOrWhiteSpace(await AskAsync()));
            var loaded = await MeasureAsync("chat model loaded and answered");

            // The clip the owner gets from Quick: 960 × 640, 121 frames, 20 steps, the fast decoder, previews.
            var request = VideoRequest.For("A mountain lake at dawn, mist drifting over the water, the camera moves slowly forward.", VideoQuality.Quick, PictureShape.Wide, seed: 77) with { Preview = true };
            var estimate = VideoEstimate.For(request);
            long peak = 0;
            using var stop = new CancellationTokenSource();
            var watch = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (await probe.MainGpuAsync() is { } gpu) peak = Math.Max(peak, gpu.TotalBytes - gpu.FreeBytes);
                    try { await Task.Delay(500, stop.Token); } catch (OperationCanceledException) { }
                }
            });
            var stages = new List<(double At, string Stage)>();
            var progress = new Collect<VideoProgress>
            {
                Then = p => { lock (stages) if (stages.Count == 0 || stages[^1].Stage != p.Stage) stages.Add((clock.Elapsed.TotalSeconds, p.Stage)); },
            };
            var start = clock.Elapsed;
            var clip = await videos.MakeAsync(setup.Model, request, progress);
            var took = (clock.Elapsed - start).TotalSeconds;
            stop.Cancel();
            await watch;

            output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] clip made in {took:0} s (estimate {estimate.Total:0} s: {estimate.Words()}; start {estimate.Start:0}, drawing {estimate.Drawing:0}, decoding {estimate.Decoding:0}, encoding {estimate.Encoding:0}); {clip.Webm.Length:N0} bytes; card peaked at {Gb(peak)} used");
            lock (stages) output.WriteLine("  stages: " + string.Join(", ", stages.Select(s => $"{s.Stage} at {s.At - start.TotalSeconds:0} s")));
            output.WriteLine($"  previews: {progress.Items.Count(p => p.Preview is not null)}; the clip's file: {WebmInfo.Read(clip.Webm)}");
            Assert.Equal((960, 640, 121, 77L), (clip.Width, clip.Height, clip.Frames, clip.Seed));
            Assert.Null(VideoClips.Problem(clip.Webm, request));
            // In the time the estimate said, give or take a third.
            Assert.InRange(took, estimate.Total * 2 / 3, estimate.Total * 4 / 3);

            // The chat model was set aside, not unloaded, and the video program is gone: the card is as it was before anything.
            Assert.Equal(EngineState.Resting, engine.State);
            Assert.Equal(CardChoice.SetChatAside, card.LastDecision!.Choice);
            Assert.False(video.IsRunning);
            Assert.False(card.IsBusy);
            await Task.Delay(1500);
            var after = await MeasureAsync("after the clip");
            Assert.True(after.FreeBytes > before.FreeBytes - (1L << 30), "something stayed on the card after the clip");
            // The engine takes what the cleared card has while it decodes (23.0 of 24.0 GB here), but never the last of it.
            Assert.True(peak < loaded.TotalBytes - (1L << 29), $"the card was filled to {Gb(peak)}");

            // A chat reply straight after a clip: the model loads again by itself and answers.
            var asked = Stopwatch.StartNew();
            var reply = await AskAsync();
            output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0} s] the chat model was back and had answered {asked.Elapsed.TotalSeconds:0.0} s after it was asked: {reply}");
            Assert.False(string.IsNullOrWhiteSpace(reply));
            Assert.Equal(EngineState.Ready, engine.State);
            Assert.Equal(8192, engine.ContextLength);

            await engine.UnloadAsync();
            await Task.Delay(1000);
            await MeasureAsync("everything unloaded");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// How full the card gets while a clip is made, stage by stage, with and without previews. Prints the numbers; it
    /// fails only when the card is filled to its last gigabyte, where Windows starts lending shared memory and
    /// everything crawls. Clips of the real length at Quick's size with 8 steps (the peaks do not depend on the steps).
    /// </summary>
    [Fact]
    public async Task The_card_is_never_filled_while_a_clip_is_made()
    {
        if (Setup() is not { } setup) return;
        var root = Path.Combine(Path.GetTempPath(), "dogai-videomem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            using var http = new LoopbackHttp();
            using var server = new VideoServer(http, new SdServerLocator(paths), paths);
            var probe = new HardwareProbe();
            var total = (await probe.MainGpuAsync())!.TotalBytes;

            foreach (var (name, request) in new[]
            {
                ("Quick, fast decoder, previews", VideoRequest.For("A mountain lake at dawn, mist drifting over the water.", VideoQuality.Quick, PictureShape.Wide, seed: 5) with { Steps = 8, Preview = true }),
                ("Quick, fast decoder, no previews", VideoRequest.For("A mountain lake at dawn, mist drifting over the water.", VideoQuality.Quick, PictureShape.Wide, seed: 5) with { Steps = 8 }),
                ("Quick, own decoder, previews", VideoRequest.For("A mountain lake at dawn, mist drifting over the water.", VideoQuality.Quick, PictureShape.Wide, seed: 5) with { Steps = 8, Preview = true, Decoder = VideoDecoder.Own }),
            })
            {
                var peaks = new Dictionary<string, long>();
                var order = new List<string>();
                var stage = "before";
                long shared = 0;
                using var stop = new CancellationTokenSource();
                var watch = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (await probe.MainGpuAsync() is { } gpu)
                        {
                            var now = stage;
                            lock (peaks)
                            {
                                if (!peaks.ContainsKey(now)) order.Add(now);
                                peaks[now] = Math.Max(peaks.GetValueOrDefault(now), gpu.TotalBytes - gpu.FreeBytes);
                            }
                        }
                        if (server.ProcessId is int pid) shared = Math.Max(shared, await SharedOfProcessAsync(pid));
                        try { await Task.Delay(150, stop.Token); } catch (OperationCanceledException) { }
                    }
                });
                var progress = new Collect<VideoProgress> { Then = p => stage = p.Stage };
                var clock = Stopwatch.StartNew();
                var clip = await server.MakeAsync(setup.Model, request, progress);
                stop.Cancel();
                await watch;
                var peak = peaks.Values.Max();
                output.WriteLine($"{name}: {clock.Elapsed.TotalSeconds:0} s, {clip.Webm.Length:N0} bytes; card peak {Gb(peak)} of {Gb(total)}; shared memory peak {Gb(shared)}");
                output.WriteLine("  " + string.Join(", ", order.Select(s => $"{s} {Gb(peaks[s])}")));
                Assert.True(peak < total - (1L << 29), $"{name}: the card was filled to {Gb(peak)} of {Gb(total)}");
                Assert.True(shared < 1L << 29, $"{name}: the video program borrowed {Gb(shared)} of shared memory");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The shared (borrowed from RAM) graphics memory of one program, from Windows' own counter; 0 when it cannot be read.</summary>
    private static async Task<long> SharedOfProcessAsync(int pid)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"(Get-Counter '\\GPU Process Memory(pid_{pid}_*)\\Shared Usage' -ErrorAction SilentlyContinue).CounterSamples | Measure-Object CookedValue -Sum | ForEach-Object Sum\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                },
            };
            process.Start();
            var text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return long.TryParse(text.Trim().Split('.', ',')[0], out var bytes) ? bytes : 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// For looking at a clip by hand: with DOGAI_TEST_VIDEO_OUT set to a folder the clips are saved there. Off
    /// otherwise, so a normal run writes no clip anywhere.
    /// </summary>
    private static void Save(byte[] webm, string name)
    {
        if (Environment.GetEnvironmentVariable("DOGAI_TEST_VIDEO_OUT") is { Length: > 0 } folder && Directory.Exists(folder))
            File.WriteAllBytes(Path.Combine(folder, name), webm);
    }

    /// <summary>The files of a folder with their sizes and dates: anything written or changed shows.</summary>
    private static List<string> Listing(string folder) =>
        [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f)).Select(f => $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}").Order(StringComparer.OrdinalIgnoreCase)];

    private static bool IsPng(byte[] data) => data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G';

    /// <summary>True when the bytes hold the word in any case, as plain text or as two-byte text.</summary>
    private static bool Holds(byte[] data, string word)
    {
        var text = Encoding.Latin1.GetString(data);
        if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        var wide = Encoding.Latin1.GetString(Encoding.Unicode.GetBytes(word));
        return text.Contains(wide, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<uint> Masters =
    [
        0x1A45DFA3, 0x18538067, 0x114D9B74, 0x4DBB, 0x1549A966, 0x1654AE6B, 0xAE, 0xE0, 0x1C53BB6B, 0xBB, 0xB7, 0x1F43B675, 0xA0,
        0x1254C367, 0x7373, 0x63C0, 0x67C8, 0x1941A469, 0x61A7, 0x1043A770,
    ];

    /// <summary>
    /// The parts of a WebM as a list of ids and sizes, with the value of small ones: for looking at what the engine
    /// writes when the structure check refuses a clip. Frames are counted, not listed.
    /// </summary>
    internal static string Tree(byte[] data)
    {
        var text = new StringBuilder();
        var blocks = 0;
        Walk(0, data.Length, 0);
        text.Append($"({blocks} frames)");
        return text.ToString();

        void Walk(int at, int end, int depth)
        {
            while (at < end)
            {
                var first = data[at];
                var idLength = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : 0;
                if (idLength == 0 || at + idLength >= end) { text.AppendLine($"{new string(' ', depth * 2)}?? at {at}"); return; }
                uint id = 0;
                for (var i = 0; i < idLength; i++) id = id << 8 | data[at + i];
                at += idLength;
                var lead = data[at];
                if (lead == 0) { text.AppendLine($"{new string(' ', depth * 2)}?? size at {at}"); return; }
                var sizeLength = System.Numerics.BitOperations.LeadingZeroCount((uint)lead) - 23;
                long size = lead & (0xFF >> sizeLength);
                var unknown = size == (0xFF >> sizeLength);
                for (var i = 1; i < sizeLength; i++) { size = size << 8 | data[at + i]; unknown &= data[at + i] == 0xFF; }
                at += sizeLength;
                var bodyEnd = unknown ? end : (int)Math.Min(end, at + size);
                if (id is 0xA3 or 0xA1) { blocks++; at = bodyEnd; continue; }
                var value = !Masters.Contains(id) && bodyEnd - at <= 40 ? " = " + Convert.ToHexString(data.AsSpan(at, bodyEnd - at)) + " \"" + new string([.. data.AsSpan(at, bodyEnd - at).ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.')]) + "\"" : "";
                if (id != 0xE7 && id != 0xA0 && id != 0xBB && id != 0xB7 && id != 0xB3 && id != 0xF7 && id != 0xF1 && id != 0xF0 && id != 0x5378)
                    text.AppendLine($"{new string(' ', depth * 2)}{id:X} size {(unknown ? "unknown" : size.ToString())}{value}");
                if (Masters.Contains(id)) Walk(at, bodyEnd, depth + 1);
                at = bodyEnd;
            }
        }
    }
}
