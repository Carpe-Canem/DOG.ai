using System.Text.Json;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Video;
using DogAI.Engine;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// "Write it for me…" for video with a real model on the real engine (M21 step 4). Skipped unless DOGAI_TEST_MODEL
/// points at a chat model. The trial's six ideas: the three from words always; the three from a first frame only when
/// the model's projector is beside it and DOGAI_TEST_VIDEO_FRAMES is a folder holding lake.jpg, girl.jpg and wolf.jpg
/// (JPEGs as the page makes them: at most 1,024 pixels, nothing but the picture). Each idea is written three times and
/// printed (--logger "console;verbosity=detailed"); with DOGAI_TEST_VIDEO_SENTENCES set, the first sentence of each idea
/// is written to that file as JSON (the trial's clips are made from it).
/// </summary>
public class VideoWriterSmokeTests(ITestOutputHelper output)
{
    /// <summary>The trial's six ideas: the owner's plain words, and the first frame's file for three of them.</summary>
    public static readonly IReadOnlyList<(string Id, string Words, string? Frame)> Ideas =
    [
        ("lighthouse", "a lighthouse on a cliff in a storm, big waves", null),
        ("lake", "the waterfall falls into the lake and mist rises", "lake.jpg"),
        ("street", "a woman in a red coat walking down a snowy street at night", null),
        ("girl", "the wind blows her hair and she smiles", "girl.jpg"),
        ("fox", "an anthro fox playing a lute in a tavern", null),
        ("wolf", "he turns his head to the fire", "wolf.jpg"),
    ];

    [Fact]
    public async Task A_real_model_writes_a_clip_sentence_from_words_and_from_a_frame()
    {
        var model = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        if (string.IsNullOrEmpty(model)) return;
        var projector = ModelProjector.Find(model);
        var frames = Environment.GetEnvironmentVariable("DOGAI_TEST_VIDEO_FRAMES");

        using var http = new LoopbackHttp();
        using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
        await engine.LoadAsync(model, new EngineOptions(8192, -1, projector?.Path));
        var writer = new VideoWriter(engine);
        var first = new Dictionary<string, string>();
        var refused = 0;

        foreach (var (id, words, frameFile) in Ideas)
        {
            byte[]? jpeg = null;
            if (frameFile is not null)
            {
                if (projector is null || string.IsNullOrEmpty(frames) || !File.Exists(Path.Combine(frames, frameFile))) continue;
                Assert.True(engine.SeesPictures, engine.Error);
                jpeg = AttachedPictures.Plain(await File.ReadAllBytesAsync(Path.Combine(frames, frameFile)));
                Assert.Null(AttachedPictures.Problem(jpeg));
            }
            for (var run = 1; run <= 3; run++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                VideoDraft draft;
                try
                {
                    draft = jpeg is null ? await writer.FromWordsAsync(words) : await writer.FromFrameAsync(jpeg, words);
                }
                catch (InvalidOperationException e)
                {
                    // The dialog says it and offers Write it again; counted here, not stopped at.
                    refused++;
                    output.WriteLine($"{id} #{run} ({clock.Elapsed.TotalSeconds:0.0} s): REFUSED: {e.Message}");
                    continue;
                }
                output.WriteLine($"{id} #{run} ({clock.Elapsed.TotalSeconds:0.0} s): {draft.Sentence}");
                output.WriteLine($"  seen: {draft.Seen} | moves: {draft.Moves} | camera: {draft.Camera}");
                Assert.InRange(draft.Sentence.Length, 20, VideoWriter.MaxSentenceLength + 1);
                Assert.True(VideoWriter.MostlyLatin(draft.Sentence));
                first.TryAdd(id, draft.Sentence);
            }
        }
        await engine.UnloadAsync();
        output.WriteLine($"refused: {refused}");
        Assert.True(refused <= 2, $"{refused} answers were refused");

        if (Environment.GetEnvironmentVariable("DOGAI_TEST_VIDEO_SENTENCES") is { Length: > 0 } file)
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(first, new JsonSerializerOptions { WriteIndented = true }));
    }
}
