using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Engine;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// "Draft from the portrait" with a real model on the real engine (M19 step 2). Skipped unless DOGAI_TEST_MODEL points
/// at a chat model with its projector beside it and DOGAI_TEST_PORTRAITS at a folder of JPEGs as DOG.ai makes them
/// (at most 1,024 pixels, nothing but the picture: save a portrait's JPEG from the page). Each is drafted four times; the
/// Looks are printed (--logger "console;verbosity=detailed") and must hold no clothing, pose, expression, place or light.
/// </summary>
public class LooksFromPortraitSmokeTests(ITestOutputHelper output)
{
    /// <summary>Tags that are never lasting features: if one comes back, the form was not followed.</summary>
    private static readonly HashSet<string> NotLooks = new(StringComparer.OrdinalIgnoreCase)
    {
        "dress", "shirt", "skirt", "jacket", "coat", "armor", "cape", "hat", "boots", "gloves", "hood", "sweater", "white dress", "red cape", "straw hat",
        "smile", "frown", "blush", "open mouth", "closed eyes", "sitting", "standing", "looking at viewer", "holding", "holding sword",
        "forest", "castle", "field", "sunflower", "outdoors", "indoors", "night", "sunlight", "day", "sky", "blue sky", "portrait", "upper body",
    };

    [Fact]
    public async Task A_real_model_drafts_lasting_features_from_portraits()
    {
        var model = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        var folder = Environment.GetEnvironmentVariable("DOGAI_TEST_PORTRAITS");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(folder) || ModelProjector.Find(model) is not { } projector) return;
        var portraits = Directory.GetFiles(folder, "*.jpg").Order().ToList();
        Assert.NotEmpty(portraits);

        using var http = new LoopbackHttp();
        using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
        await engine.LoadAsync(model, new EngineOptions(8192, -1, projector.Path));
        Assert.True(engine.SeesPictures, engine.Error);
        var heard = new Heard(engine);
        var writer = new PictureWriter(heard);

        foreach (var file in portraits)
        {
            var jpeg = AttachedPictures.Plain(await File.ReadAllBytesAsync(file));
            Assert.Null(AttachedPictures.Problem(jpeg));
            for (var run = 1; run <= 4; run++)
            {
                var draft = await writer.DraftLooksFromPictureAsync(jpeg, "", PromptStyle.Tags, TagList.Bundled);
                output.WriteLine($"{Path.GetFileName(file)} #{run}: {draft.Tags}");
                output.WriteLine("  answered: " + heard.Last.ReplaceLineEndings(" "));
                if (draft.Notes.Dropped.Count > 0) output.WriteLine("  left out: " + string.Join(", ", draft.Notes.Dropped));
                var tags = draft.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                Assert.Matches("^1(girl|boy|other)$", tags[0]);
                Assert.True(tags.Length >= 4, draft.Tags);
                Assert.DoesNotContain(tags, NotLooks.Contains);
            }
        }
        await engine.UnloadAsync();
    }

    /// <summary>The engine as it is, keeping the last answer so the test can print what the model filled in.</summary>
    private sealed class Heard(IChatEngine inner) : IChatEngine
    {
        public string Last { get; private set; } = "";
        public bool IsReady => inner.IsReady;
        public bool SeesPictures => inner.SeesPictures;
        public string? ModelName => inner.ModelName;
        public int ContextLength => inner.ContextLength;
        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) => inner.CountTokensAsync(text, ct);
        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) => inner.RenderPromptAsync(messages, ct);
        public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default) => inner.StreamCompletionAsync(prompt, sampler, ct);

        public async IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var text = new System.Text.StringBuilder();
            await foreach (var delta in inner.StreamChatAsync(messages, sampler, ct))
            {
                text.Append(delta.Content);
                yield return delta;
            }
            Last = text.ToString();
        }
    }
}
