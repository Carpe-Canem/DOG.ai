using System.Diagnostics;
using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Engine;
using DogAI.Engine.Images;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// Draws real pictures with the real picture engine. Skipped unless DOGAI_SD_SERVER points at sd-server.exe and
/// DOGAI_TEST_IMAGE_MODEL at an SDXL .safetensors checkpoint; DOGAI_TEST_IMAGE_LORAS may name a folder of LoRAs.
/// Needs about 12 GB of free graphics memory and takes about half a minute.
/// </summary>
public class ImageSmokeTests(ITestOutputHelper output)
{
    public const string ModelVariable = "DOGAI_TEST_IMAGE_MODEL";
    public const string LorasVariable = "DOGAI_TEST_IMAGE_LORAS";
    private const string Marker = "zqxmarkerword";

    [Fact]
    public async Task Draws_previews_cancels_uses_a_lora_and_unloads()
    {
        var exe = Environment.GetEnvironmentVariable(SdServerLocator.OverrideVariable);
        var checkpoint = Environment.GetEnvironmentVariable(ModelVariable);
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(checkpoint)) return;
        var loras = Environment.GetEnvironmentVariable(LorasVariable) is { Length: > 0 } l ? l : null;

        var root = Path.Combine(Path.GetTempPath(), "dogai-sdsmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engineFolder = Path.GetDirectoryName(exe)!;
        var engineFilesBefore = Directory.GetFiles(engineFolder, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        try
        {
            var paths = new AppPaths(root);
            using var http = new LoopbackHttp();
            using var server = new ImageServer(http, new SdServerLocator(paths), paths);
            server.Output += line => { lock (lines) lines.Add(line); };

            var clock = Stopwatch.StartNew();
            await server.LoadAsync(new ImageModel(checkpoint, loras));
            Assert.True(server.IsRunning, server.Error);
            output.WriteLine($"started in {clock.ElapsedMilliseconds} ms");

            // Its own web page is replaced by DOG.ai's blank one.
            var page = await http.Client.GetStringAsync($"http://127.0.0.1:{server.Port}/");
            Assert.Contains("<title>DOG.ai</title>", page);

            var caps = await server.CapabilitiesAsync();
            Assert.Contains("euler_a", caps.Samplers);
            output.WriteLine($"samplers: {string.Join(", ", caps.Samplers)}; loras: {string.Join(", ", caps.Loras.Select(x => x.Path))}");

            // 1. A Draft picture with previews.
            var progress = new Collect<ImageProgress>();
            clock.Restart();
            var draft = new ImageRequest($"1girl, silver hair, green eyes, forest, {Marker}", "lowres, bad anatomy", 832, 1216, Seed: 1234,
                Steps: 20, Sampler: "euler_a", Guidance: 5.5, ClipSkip: 2, Preview: true, PreviewEvery: 4);
            var first = await server.DrawAsync(draft, progress);
            output.WriteLine($"draft: {clock.ElapsedMilliseconds} ms, {first.Images[0].Length} bytes; stages {string.Join(",", progress.Items.Select(p => p.Stage).Distinct())}");
            Assert.Equal(1234, first.Seed);
            Assert.True(IsPng(Assert.Single(first.Images)));
            Assert.Equal((832, 1216), PngSize(first.Images[0]));
            // Progress is asked for four times a second and the engine does about five steps a second, so not every step is seen.
            var sampling = progress.Items.Where(p => p.Stage == "sampling").ToList();
            Assert.All(sampling, p => Assert.Equal(20, p.TotalSteps));
            Assert.InRange(sampling.Max(p => p.Step ?? 0), 15, 20);
            Assert.True(sampling.Count >= 8, $"only {sampling.Count} progress reports while sampling");
            var previews = progress.Items.Where(p => p.Preview is not null).ToList();
            Assert.NotEmpty(previews);
            Assert.All(previews, p => Assert.True(IsPng(p.Preview!)));
            Assert.False(server.IsBusy);

            // 2. The same seed and settings give the same picture (what "Edit and redraw" will rely on).
            var again = await server.DrawAsync(draft);
            Assert.True(first.Images[0].AsSpan().SequenceEqual(again.Images[0]), "the same seed gave a different picture");

            // 3. Cancel mid-picture, then draw again normally.
            using (var cts = new CancellationTokenSource())
            {
                var watch = new Collect<ImageProgress> { Then = p => { if (p.Stage == "sampling" && p.Step >= 3) cts.Cancel(); } };
                clock.Restart();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.DrawAsync(draft with { Steps = 60, Preview = false }, watch, cts.Token));
                output.WriteLine($"cancel came back after {clock.ElapsedMilliseconds} ms from the start");
            }

            // 4. A LoRA, WebP output, random seed.
            var lora = caps.Loras.FirstOrDefault();
            var webp = await server.DrawAsync(draft with
            {
                Seed = -1, Steps = 12, Format = ImageFormat.Webp, Quality = 90,
                Loras = lora.Path is { } path ? [new LoraUse(path, 0.8)] : null,
            });
            Assert.True(webp.Seed >= 0);
            Assert.True(IsWebp(Assert.Single(webp.Images)));
            output.WriteLine($"webp with lora {lora.Path ?? "(none)"}: {webp.Images[0].Length} bytes, seed {webp.Seed}");

            // 5. Hires fix doubles nothing it should not: 768×768 at 1.5 gives 1152×1152.
            var hires = await server.DrawAsync(new ImageRequest("1girl, portrait", Width: 768, Height: 768, Seed: 5, Steps: 8, ClipSkip: 2,
                Hires: new HiresFix(1.5, 4, 0.5)));
            Assert.Equal((1152, 1152), PngSize(hires.Images[0]));

            // 6. Unload ends the program and gives the memory back.
            await server.UnloadAsync();
            Assert.Null(server.Model);
            Assert.False(server.IsRunning);
            await Assert.ThrowsAsync<ImageEngineException>(() => server.DrawAsync(draft));
        }
        finally
        {
            lock (lines)
            {
                output.WriteLine($"{lines.Count} output lines");
                // The prompt never shows up in what the engine prints at its normal level.
                Assert.DoesNotContain(lines, line => line.Contains(Marker, StringComparison.OrdinalIgnoreCase));
            }
            // It wrote nothing beside itself except DOG.ai's blank page (when it runs from DOG.ai's own engine folder).
            var added = Directory.GetFiles(engineFolder, "*", SearchOption.AllDirectories).Where(f => !engineFilesBefore.Contains(f)).ToList();
            Assert.Empty(added);
            var leftInRoot = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("dogai-blank.html", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Empty(leftInRoot);
            Directory.Delete(root, true);
        }
    }

    private static bool IsPng(byte[] data) => data.Length > 24 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G';

    private static bool IsWebp(byte[] data) =>
        data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';

    private static (int Width, int Height) PngSize(byte[] data) =>
        ((data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19], (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23]);
}
